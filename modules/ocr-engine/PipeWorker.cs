using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using SkiaSharp;

internal static class PipeWorker
{
    internal const int Magic = 0x514A4F33;
    internal static int Execute(string pipeName)
    {
        if (!pipeName.StartsWith("QingJie-OCR-", StringComparison.Ordinal) || !Guid.TryParseExact(pipeName[12..], "N", out _)) return 2;
        // Parent cancellation closes the pipe and kills this disposable process.
        // This final watchdog also bounds a crash during a native inference.
        using var watchdog = new Timer(_ => Environment.Exit(3), null, 600000, Timeout.Infinite);
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.None, System.Security.Principal.TokenImpersonationLevel.Identification);
        try
        {
            pipe.Connect(10000);
            using var reader = new BinaryReader(pipe, Encoding.UTF8, true);
            using var writer = new BinaryWriter(pipe, Encoding.UTF8, true);
            ScreenPage page;
            try
            {
                if (reader.ReadInt32() != Magic) throw new InvalidDataException("Invalid OCR protocol");
                int width = reader.ReadInt32(), height = reader.ReadInt32();
                if (width <= 0 || height <= 0 || (long)width * height > 64000000) throw new InvalidDataException("Invalid screenshot dimensions");
                if (reader.ReadString().Length != 0) throw new InvalidDataException("This model supports Chinese and English; use the selected language engine.");
                reader.ReadString(); // Legacy optional-language directory, not used by this model.
                using var bitmap = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
                var strip = new byte[checked(width * 4 * Math.Min(32, height))];
                for (int y = 0; y < height; y += 32)
                {
                    int length = width * 4 * Math.Min(32, height - y), offset = 0;
                    while (offset < length) { int read = reader.Read(strip, offset, length - offset); if (read == 0) throw new EndOfStreamException(); offset += read; }
                    Marshal.Copy(strip, 0, IntPtr.Add(bitmap.GetPixels(), y * bitmap.RowBytes), length);
                }
                using var engine = new ScreenOcr();page = engine.Read(bitmap, CancellationToken.None);
            }
            catch (Exception error) { writer.Write(Magic);writer.Write(error.Message);writer.Flush();return 1; }
            writer.Write(Magic);writer.Write("");writer.Write("");writer.Write(page.Words.Length);
            foreach (var word in page.Words)
            {
                writer.Write(word.Text);writer.Write(word.Line);Box(writer, word.Box);writer.Write(word.Prefix);writer.Write(word.Confidence);
            }
            writer.Write(page.Paragraphs.Length);
            foreach (var paragraph in page.Paragraphs)
            {
                writer.Write(paragraph.Text);Box(writer, paragraph.Box);writer.Write(paragraph.Lines.Length);
                foreach (var line in paragraph.Lines) { writer.Write(line.Text);Box(writer, line.Box); }
            }
            writer.Flush();return 0;
        }
        catch { return 1; }
    }
    private static void Box(BinaryWriter writer, SKRect box) { writer.Write((double)box.Left);writer.Write((double)box.Top);writer.Write((double)box.Width);writer.Write((double)box.Height); }
}
