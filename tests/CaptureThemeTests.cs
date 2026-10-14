using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Mori;
using QingJie;

namespace MoriDesk {
 internal static class CaptureThemeTests {
  static IEnumerable<DependencyObject> All(DependencyObject root){yield return root;foreach(object child in LogicalTreeHelper.GetChildren(root)){var value=child as DependencyObject;if(value!=null)foreach(var nested in All(value))yield return nested;}}
  static Color ColorOf(Brush brush){return ((SolidColorBrush)brush).Color;}
  static bool Token(Brush brush,string name){return ColorOf(brush)==ColorOf(Theme.Brush(name));}
  static byte[] Pixels(BitmapSource source){var converted=new FormatConvertedBitmap(source,PixelFormats.Bgra32,null,0);var pixels=new byte[source.PixelWidth*source.PixelHeight*4];converted.CopyPixels(pixels,source.PixelWidth*4,0);return pixels;}
  static void Render(Window window,int width,int height,string file){var body=(FrameworkElement)window.Content;body.Measure(new Size(width,height));body.Arrange(new Rect(0,0,width,height));body.UpdateLayout();var bitmap=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);bitmap.Render(body);ImageFiles.Write(bitmap,file);}
  internal static void Run(string root,Action<bool,string> check){
   Directory.CreateDirectory(root);string previousTheme=Theme.Current;var previousSettings=AppState.Settings;var previousPins=AppState.Pins;
   WriteTranslationWindow writer=null;TextWindow text=null;QingJie.SettingsWindow settings=null;PinWindow pin=null;Window chromeProbe=null;
   try{
    Theme.Set("light");AppState.Settings=new Preferences();AppState.Pins=new PinStore(Path.Combine(root,"pins"));
    var sample=new byte[64*32*4];new Random(902).NextBytes(sample);for(int i=3;i<sample.Length;i+=4)sample[i]=255;
    var image=BitmapSource.Create(64,32,96,96,PixelFormats.Bgra32,null,sample,64*4);image.Freeze();byte[] originalPixels=Pixels(image);
    int requests=0;writer=new WriteTranslationWindow((source,cancel,language)=>{requests++;return Task.FromResult("not requested");});
    var input=(TextBox)LogicalTreeHelper.FindLogicalNode(writer,"TranslationInput");var output=(TextBox)LogicalTreeHelper.FindLogicalNode(writer,"TranslationOutput");var primary=(Button)LogicalTreeHelper.FindLogicalNode(writer,"TranslateInput");var writerFrame=(Border)LogicalTreeHelper.FindLogicalNode(writer,"TranslationFrame");input.Text="Keep this selected input";input.Select(5,4);
    text=new TextWindow(image,"zh",(source,cancel)=>Task.FromResult(new OcrPage {Text="Keep this transcript",Words=new List<WordBox>()}));
    var transcript=(TextBox)LogicalTreeHelper.FindLogicalNode(text,"RecognizedText");var result=(TextBox)LogicalTreeHelper.FindLogicalNode(text,"SelectedTranslation");var picture=(Image)LogicalTreeHelper.FindLogicalNode(text,"OcrPicture");var toolbar=(DockPanel)LogicalTreeHelper.FindLogicalNode(text,"OcrToolbar");
    var resultCard=((FrameworkElement)result.Parent).Parent as Border;
    transcript.Text="Keep this transcript";transcript.Select(0,4);
    var selection=new OcrLanguageSelection(AppState.Settings,()=>{},id=>id=="kor",id=>{});settings=new QingJie.SettingsWindow(selection);
    pin=new PinWindow(AppState.Pins,new PinRecord {Id=Guid.NewGuid().ToString("N")},image);
    var pinImage=(Image)LogicalTreeHelper.FindLogicalNode(pin,"PinPicture");var pinStatus=(Border)LogicalTreeHelper.FindLogicalNode(pin,"PinTranslationStatus");
    var tool=Ui.Tool("rect","test tool",()=>{});var card=Ui.Card(new TextBlock {Text="test card"});var active=Ui.Tool("arrow","active tool",()=>{});Ui.SetActive(active,true);var menu=Ui.Menu(tool);
    // A chrome mapper must never rewrite local source bytes or annotation colour.
    var untouched=new TextBox {Foreground=Ui.Brush("#EF4444")};var mixed=new StackPanel();mixed.Children.Add(new Image {Source=image});mixed.Children.Add(untouched);Ui.ThemeControls(mixed);
    // Application resources invalidate live logical trees, not detached controls.
    // Keep this tree rooted in a real Window without opening a native surface.
    var chrome=new StackPanel();chrome.Children.Add(tool);chrome.Children.Add(card);chrome.Children.Add(active);chrome.Children.Add(mixed);tool.ContextMenu=menu;chromeProbe=new Window {Content=chrome};
    var regions=new[]{new TranslationRegion {Text="one",Box=new Rect(8,8,40,18)}};
    byte[] translatedPixels=Pixels(ImageTranslation.Render(image,regions,new[]{"测试"}));
    foreach(string appearance in new[]{"light","dark","light"}){
     Theme.Set(appearance);
     check(Token(input.Foreground,"Text")&&Token(output.Foreground,"Text")&&Token(input.SelectionBrush,"AccentSoft"),"existing input/result and selection colours follow live "+appearance+" tokens");
     check(Token(primary.Background,"Primary")&&Token(primary.Foreground,"OnPrimary"),"translation primary action retains contrast-safe purple/on-purple colours in "+appearance);
     check(Token(writer.Background,"Surface")&&Token(writerFrame.Background,"Surface")&&Token(writerFrame.BorderBrush,"Border"),"unshown/unsupported-blur translation window has a readable themed fallback in "+appearance);
     check(Token(text.Background,"Canvas")&&Token(toolbar.Background,"Panel")&&Token(transcript.Background,"Input")&&Token(transcript.Foreground,"Text")&&Token(result.Background,"Input")&&Token(result.Foreground,"Text"),"OCR chrome, transcript and selected translation use live "+appearance+" tokens");
     check(resultCard!=null&&Token(resultCard.Background,"Surface")&&!Token(resultCard.Background,"AccentSoft")&&!Token(result.Background,"AccentSoft")&&Token(result.SelectionBrush,"AccentSoft"),"large translation card stays neutral; purple is limited to text selection in "+appearance);
     var headings=All(settings).OfType<TextBlock>().Where(block=>block.Text=="识别语言"||block.Text=="离线中英包 · 可选").ToArray();
     check(headings.Length==2&&Token(settings.Background,"Canvas")&&headings.All(block=>Token(block.Foreground,"Text")),"capture settings headings follow "+appearance+" without replacing pack/selection controls");
     var saveButtons=All(settings).OfType<Button>().Where(button=>Convert.ToString(button.Content)=="保存快捷键").ToArray();
     check(saveButtons.Length==1&&saveButtons.All(button=>Token(button.Background,"Primary")&&Token(button.Foreground,"OnPrimary")),"embedded capture primary button is not flattened into neutral colours in "+appearance);
     var provider=All(settings).OfType<ComboBox>().Single(combo=>combo.Name=="TranslationProvider");check(Token(provider.Background,"Input")&&Token(provider.Foreground,"Text")&&Token((Brush)provider.Resources[SystemColors.WindowBrushKey],"Surface")&&Token((Brush)provider.Resources[SystemColors.WindowTextBrushKey],"Text"),"native translation selector and its dropdown remain readable in "+appearance);
     check(Token(tool.Foreground,"Text")&&Token(card.Background,"Surface")&&Token(card.BorderBrush,"Border")&&Token(active.Background,"AccentSoft"),"tool icons, floating cards and active states follow live "+appearance+" tokens");
     check(Token(menu.Background,"Surface")&&Token(menu.Foreground,"Text")&&Token(menu.BorderBrush,"Border")&&Token((Brush)menu.Resources[SystemColors.WindowBrushKey],"Surface")&&Token((Brush)menu.Resources[SystemColors.WindowTextBrushKey],"Text"),"existing detached context menu and popup system brushes follow "+appearance+" tokens");
     check(Token(pinStatus.Background,"Surface")&&Token(((TextBlock)pinStatus.Child).Foreground,"Text"),"pin translation status has matching readable foreground/background in "+appearance);
     check(input.Text=="Keep this selected input"&&input.SelectionStart==5&&input.SelectionLength==4&&transcript.SelectedText=="Keep"&&requests==0,"theme switches preserve selected input/OCR text and never invoke translation in "+appearance);
     check(ReferenceEquals(picture.Source,image)&&ReferenceEquals(pinImage.Source,image)&&Pixels(image).SequenceEqual(originalPixels)&&ColorOf(untouched.Foreground)==(Color)ColorConverter.ConvertFromString("#EF4444"),"theme adaptation never replaces image sources, bitmap bytes or explicit annotation colour in "+appearance);
     check(Pixels(ImageTranslation.Render(image,regions,new[]{"测试"})).SequenceEqual(translatedPixels)&&ColorOf(Ui.Ink)==(Color)ColorConverter.ConvertFromString("#303639"),"rendered image translation pixels remain identical across "+appearance+" UI themes");
     if(appearance=="dark"||!File.Exists(Path.Combine(root,"writer-light.png"))){Render(writer,400,280,Path.Combine(root,"writer-"+appearance+".png"));Render(text,1080,580,Path.Combine(root,"ocr-"+appearance+".png"));}
    }
    Ui.SetActive(active,false);check(active.Tag==null&&ColorOf(active.Background).A==0,"clearing an active tool restores a transparent, unselected state");
    var overrideTarget=new Border();var localInk=Ui.Brush("#EF4444");overrideTarget.Resources["Text"]=localInk;var overrideMenu=Ui.Menu(overrideTarget);check(ReferenceEquals(overrideMenu.Foreground,localInk),"context menu target overrides retain precedence over shared application tokens");
    check(!writer.IsVisible&&!text.IsVisible&&!settings.IsVisible&&!pin.IsVisible&&!chromeProbe.IsVisible,"theme regression creates no visible windows or native desktop backdrop");
   }finally{
    if(writer!=null)writer.Close();if(text!=null)text.Close();if(settings!=null)settings.Close();if(pin!=null)pin.Close();if(chromeProbe!=null)chromeProbe.Close();AppState.Pins=previousPins;AppState.Settings=previousSettings;Theme.Set(previousTheme);
   }
  }
 }
}
