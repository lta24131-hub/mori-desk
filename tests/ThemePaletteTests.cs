using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Mori;

namespace MoriDesk {
 internal static class ThemePaletteTests {
  static double Channel(byte value){double v=value/255.0;return v<=.04045?v/12.92:Math.Pow((v+.055)/1.055,2.4);}
  static double Luminance(Color c){return .2126*Channel(c.R)+.7152*Channel(c.G)+.0722*Channel(c.B);}
  static Color ColorOf(string key){return ((SolidColorBrush)Theme.Brush(key)).Color;}
  static double Contrast(string a,string b){double x=Luminance(ColorOf(a)),y=Luminance(ColorOf(b));return (Math.Max(x,y)+.05)/(Math.Min(x,y)+.05);}
  internal static void Run(string root,Action<bool,string> check){
   Directory.CreateDirectory(root);string previous=Theme.Current;Window resourceHost=null;System.Drawing.Bitmap source=null;PreviewPicture picture=null;
   try{
    var label=new TextBlock();label.SetResourceReference(TextBlock.ForegroundProperty,"Accent");
    // WPF refreshes application resources through window logical trees.
    // Use a real, unshown host rather than asserting on an orphaned control.
    resourceHost=new Window {Content=label};
    Theme.Set("light");source=new System.Drawing.Bitmap(1,1);picture=new PreviewPicture(source,new PreviewViewport(1,1));
    foreach(string theme in new[]{"light","dark"}){
     Theme.Set(theme);
     foreach(string neutral in new[]{"Canvas","Panel","Surface","Input","Control","Border","Divider","Text","Muted","Hover","ScrollThumb","OnPrimary"}){
      var c=ColorOf(neutral);
      check(c.A==255&&c.R==c.G&&c.G==c.B,theme+" ordinary "+neutral+" uses neutral white/black/grey rather than a purple wash");
     }
     foreach(string surface in new[]{"Canvas","Panel","Surface","Input","Control","Hover","AccentSoft"}){
      check(Contrast("Text",surface)>=4.5,theme+" normal text contrasts against "+surface);
      check(Contrast("Muted",surface)>=4.5,theme+" secondary text remains legible against "+surface);
     }
     check(Contrast("Accent","Canvas")>=4.5&&Contrast("Accent","AccentSoft")>=4.5,theme+" purple navigation and selected labels have readable contrast");
     check(Contrast("OnPrimary","Primary")>=4.5,theme+" primary button foreground is readable");
     check(Contrast("ErrorText","Canvas")>=4.5&&Contrast("ErrorText","Surface")>=4.5,theme+" error text is readable without losing red semantics");
     check(ColorOf("Accent").B>ColorOf("Accent").R&&ColorOf("Accent").R>ColorOf("Accent").G,theme+" Nook accent stays purple rather than legacy green");
     check(((SolidColorBrush)label.Foreground).Color==ColorOf("Accent"),theme+" already-created controls resolve changed dynamic resources");
     check(((SolidColorBrush)Theme.Brush("Canvas")).IsFrozen&&ColorOf("Canvas").A==255,theme+" canvas remains opaque with frozen lightweight brushes");
     var drawing=Theme.DrawingColor("Canvas");var canvas=ColorOf("Canvas");
     check(drawing.R==canvas.R&&drawing.G==canvas.G&&drawing.B==canvas.B&&drawing.A==canvas.A,theme+" native image host uses the same surrounding canvas without changing pixels");
     check(Application.Current.Resources[SystemColors.HighlightBrushKey]==Theme.Brush("AccentSoft")&&Application.Current.Resources[SystemColors.HighlightTextBrushKey]==Theme.Brush("Text"),theme+" system text selections use matching foreground and purple tint");
     var menu=picture.ContextMenuStrip;var renderer=(System.Windows.Forms.ToolStripProfessionalRenderer)menu.Renderer;
     check(menu.BackColor==Theme.DrawingColor("Surface")&&menu.ForeColor==Theme.DrawingColor("Text")&&menu.Items.Cast<System.Windows.Forms.ToolStripItem>().All(item=>item.ForeColor==menu.ForeColor),theme+" native image background menu follows current surfaces and readable text");
     check(renderer.ColorTable.MenuItemSelected==Theme.DrawingColor("Hover")&&renderer.ColorTable.CheckBackground==Theme.DrawingColor("AccentSoft"),theme+" native image menu hover stays neutral while checked state retains a small purple accent");
     var highlight=(SolidColorBrush)typeof(PdfTextSelection).GetField("Highlight",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Static).GetValue(null);
     check(highlight.IsFrozen&&highlight.Color==Color.FromArgb(72,98,96,197),theme+" PDF text selection uses translucent purple while document paper remains unchanged");
     RenderShell(root,theme,check);
    }
   }finally{if(resourceHost!=null)resourceHost.Close();if(picture!=null)picture.Dispose();if(source!=null)source.Dispose();Theme.Set(previous);}
  }
  static void RenderShell(string root,string theme,Action<bool,string> check){
   Window window;using(var stream=typeof(Entry).Assembly.GetManifestResourceStream("Mori.Main.xaml"))window=(Window)System.Windows.Markup.XamlReader.Load(stream);
   try{
    Theme.Attach(window,null);var body=(FrameworkElement)window.Content;window.Content=null;body.Resources.MergedDictionaries.Add(window.Resources);
    // Preserve the window's inherited text and canvas after detaching content
    // for offscreen capture; otherwise transparent pixels appear black.
    var shell=new Border {Child=body,Background=Theme.Brush("Canvas"),Width=1280,Height=760};
    System.Windows.Documents.TextElement.SetForeground(shell,Theme.Brush("Text"));System.Windows.Documents.TextElement.SetFontFamily(shell,window.FontFamily);System.Windows.Documents.TextElement.SetFontSize(shell,window.FontSize);
    shell.Measure(new Size(1280,760));shell.Arrange(new Rect(0,0,1280,760));shell.UpdateLayout();
    var image=new RenderTargetBitmap(1280,760,96,96,PixelFormats.Pbgra32);image.Render(shell);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(image));using(var output=File.Create(Path.Combine(root,"nook-shell-"+theme+".png")))encoder.Save(output);
    var pixel=new byte[4];image.CopyPixels(new Int32Rect(500,600,1,1),pixel,4,0);var canvas=ColorOf("Canvas");
    check(!window.IsVisible&&image.PixelWidth==1280&&pixel[3]==255&&pixel[0]==canvas.B&&pixel[1]==canvas.G&&pixel[2]==canvas.R,theme+" main shell preview preserves the actual canvas in offscreen capture without operating production");
   }finally{window.Close();}
  }
 }
}
