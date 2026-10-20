// Offline-only pixel fixture. Calls the existing desktop fixture's Row factory,
// never its Main/Run methods; no Window, Application, clipboard or screen input.
using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

static class RenderScrollMatchFixtures {
    [STAThread]
    static int Main(string[] args) {
        if(args.Length!=2)throw new ArgumentException("fixture assembly and new output directory required");
        string directory=Path.GetFullPath(args[1]);
        if(Directory.Exists(directory))throw new IOException("Use a fresh generated fixture directory.");
        var assembly=Assembly.LoadFrom(Path.GetFullPath(args[0]));
        var row=assembly.GetType("ScrollCaptureDesktopAcceptance",true).GetMethod("Row",BindingFlags.Static|BindingFlags.NonPublic);
        var content=new StackPanel {Width=985,Background=Brushes.White};
        content.SetValue(System.Windows.Documents.TextElement.FontFamilyProperty,new FontFamily("Microsoft YaHei UI"));
        for(int i=1;i<=84;i++)content.Children.Add((UIElement)row.Invoke(null,new object[]{1,i}));
        content.Measure(new Size(985,double.PositiveInfinity));content.Arrange(new Rect(0,0,985,content.DesiredSize.Height));content.UpdateLayout();
        Directory.CreateDirectory(directory);
        foreach(double offset in new[]{0,48,96,435.44,774.89}) {
            const int width=971,height=525;
            var pixels=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);
            var drawing=new DrawingVisual();
            using(var context=drawing.RenderOpen()) {
                context.DrawRectangle(Brushes.White,null,new Rect(0,0,width,height));
                context.PushTransform(new TranslateTransform(-8,-10-offset));
                context.DrawRectangle(new VisualBrush(content){Stretch=Stretch.None,AlignmentX=AlignmentX.Left,AlignmentY=AlignmentY.Top},null,new Rect(0,0,985,content.DesiredSize.Height));
                context.Pop();
            }
            pixels.Render(drawing);pixels.Freeze();
            var bytes=new byte[width*height*4];pixels.CopyPixels(bytes,width*4,0);
            for(int i=0;i<bytes.Length;i+=4){byte blue=bytes[i];bytes[i]=bytes[i+2];bytes[i+2]=blue;}
            File.WriteAllBytes(Path.Combine(directory,"frame-"+offset.ToString(CultureInfo.InvariantCulture)+".rgba"),bytes);
        }
        Console.WriteLine(directory);return 0;
    }
}
