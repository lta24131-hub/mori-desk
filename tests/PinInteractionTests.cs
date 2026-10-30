using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using QingJie;

namespace MoriDesk {
 // Isolated pin records and unshown windows only. Copy/save/text sinks capture
 // the actual menu output without using the clipboard, dialogs or network.
 internal static class PinInteractionTests {
  const BindingFlags Private=BindingFlags.Instance|BindingFlags.NonPublic;
  static IEnumerable<DependencyObject> All(DependencyObject root){yield return root;foreach(object child in LogicalTreeHelper.GetChildren(root)){var node=child as DependencyObject;if(node!=null)foreach(var item in All(node))yield return item;}}
  static IEnumerable<MenuItem> Items(ItemsControl menu){foreach(var item in menu.Items.OfType<MenuItem>()){yield return item;foreach(var child in Items(item))yield return child;}}
  static MenuItem Item(PinWindow window,string name){return Items(window.ContextMenu).Single(item=>item.Name==name);}
  static void Click(PinWindow window,string name){Item(window,name).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));}
  static void Field(PinWindow window,string name,object value){typeof(PinWindow).GetField(name,Private).SetValue(window,value);}
  static void Call(PinWindow window,string name,params object[] args){typeof(PinWindow).GetMethod(name,Private).Invoke(window,args);}
  static BitmapSource Image(byte color){var bytes=new byte[120*60*4];for(int i=0;i<bytes.Length;i+=4){bytes[i]=color;bytes[i+1]=(byte)(i/4%120);bytes[i+2]=(byte)(i/480);bytes[i+3]=255;}var image=BitmapSource.Create(120,60,96,96,PixelFormats.Bgra32,null,bytes,480);image.Freeze();return image;}
  internal static void Run(string root,Action<bool,string> check){
   root=Path.Combine(root,"pin-interaction");Directory.CreateDirectory(root);
   var oldSettings=AppState.Settings;var oldPins=AppState.Pins;bool quitting=AppState.Quitting;PinWindow window=null,translation=null;
   try{
    AppState.Settings=new Preferences();AppState.Pins=new PinStore(root);AppState.Quitting=false;var store=AppState.Pins;var image=Image(31);var pin=store.Add(image);pin.Width=240;
    byte[] original=File.ReadAllBytes(store.ImagePath(pin));window=new PinWindow(store,pin,image);var surface=(Grid)LogicalTreeHelper.FindLogicalNode(window,"PinSurface");var picture=(System.Windows.Controls.Image)LogicalTreeHelper.FindLogicalNode(window,"PinPicture");
    check(window.ContextMenu!=null&&!window.ContextMenu.IsOpen&&window.Topmost&&window.WindowStyle==WindowStyle.None&&!window.ShowInTaskbar,"pin starts as a quiet borderless topmost image with actions available only through its context menu");
    check(!All((DependencyObject)window.Content).Any(node=>node is ButtonBase||node is Popup),"pin visual tree contains no replacement toolbar, floating button or popup");
    int sources=PresentationSource.CurrentSources.Cast<PresentationSource>().Count();
    foreach(var routed in new[]{Mouse.MouseEnterEvent,Mouse.MouseMoveEvent,Mouse.MouseLeaveEvent})surface.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice,0){RoutedEvent=routed});
    check(!window.ContextMenu.IsOpen&&PresentationSource.CurrentSources.Cast<PresentationSource>().Count()==sources&&ReferenceEquals(picture.Source,image),"hovering across a pin opens no menu or native surface and does not change its pixels");
    string[] expected={"PinOpenText","PinTransforms","PinRotateClockwise","PinRotateCounterclockwise","PinFlipHorizontal","PinFlipVertical","PinResetOrientation","PinCopy","PinSave","PinClose"};
    check(Items(window.ContextMenu).Select(item=>item.Name).OrderBy(name=>name).SequenceEqual(expected.OrderBy(name=>name)),"ordinary pin context menu retains all former text, transform, copy, save and close actions without unrelated controls");
    BitmapSource copied=null,saved=null;int opened=0;window.CopyImageForTests=value=>copied=value;window.SaveImageForTests=value=>saved=value;window.OpenTextForTests=()=>opened++;
    Click(window,"PinRotateClockwise");check(pin.QuarterTurns==1&&((BitmapSource)picture.Source).PixelWidth==60&&Math.Abs(window.Width-2*PinFrame.Inset-120)<.01,"right-click rotation changes the displayed orientation while preserving image scale");
    Click(window,"PinCopy");Click(window,"PinSave");check(ReferenceEquals(copied,picture.Source)&&ReferenceEquals(saved,picture.Source)&&copied.PixelWidth==60&&copied.PixelHeight==120,"copy and save receive the currently rotated full-resolution image rather than its scaled window");
    Click(window,"PinFlipHorizontal");Click(window,"PinFlipVertical");check(pin.FlipHorizontal&&pin.FlipVertical,"both former flip actions remain reachable in the context submenu");
    Click(window,"PinResetOrientation");check(pin.QuarterTurns==0&&!pin.FlipHorizontal&&!pin.FlipVertical&&((BitmapSource)picture.Source).PixelWidth==120&&Math.Abs(window.Width-2*PinFrame.Inset-240)<.01,"reset orientation restores original axes without changing scale");
    Click(window,"PinRotateCounterclockwise");check(pin.QuarterTurns==3,"counterclockwise context action retains its direction");Click(window,"PinResetOrientation");
    Click(window,"PinOpenText");check(opened==1,"text menu invokes the existing image and full-text action once");
    double width=window.Width;var wheel=new MouseWheelEventArgs(Mouse.PrimaryDevice,0,120){RoutedEvent=Mouse.MouseWheelEvent};window.RaiseEvent(wheel);
    check(wheel.Handled&&Math.Abs((window.Width-2*PinFrame.Inset)/(width-2*PinFrame.Inset)-1.1)<.001&&Math.Abs((window.Height-2*PinFrame.Inset)/(window.Width-2*PinFrame.Inset)-.5)<.001,"ordinary mouse wheel still zooms the pin proportionally");
    window.Left+=37;window.Top+=19;window.UpdateRecord();check(pin.Left==window.Left+PinFrame.Inset&&pin.Top==window.Top+PinFrame.Inset&&pin.Width==window.Width-2*PinFrame.Inset,"moving and resizing still persist image placement rather than shadow margins");
    var translatedRecord=store.Add(image);translatedRecord.IsTranslation=true;translatedRecord.ShowTranslation=false;translation=new PinWindow(store,translatedRecord,image);var action=Item(translation,"PinTranslation");
    check(action.IsEnabled&&Convert.ToString(action.Header)=="翻译图片","uncached translation pins retain an explicit right-click retry action");
    var translated=Image(211);Field(translation,"translated",translated);Call(translation,"RefreshImage",false);translation.ContextMenu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent));
    check(Convert.ToString(action.Header)=="显示译图","translation context menu describes the next action rather than an ambiguous toggle");Click(translation,"PinTranslation");
    var translatedPicture=(System.Windows.Controls.Image)LogicalTreeHelper.FindLogicalNode(translation,"PinPicture");check(translatedRecord.ShowTranslation&&ReferenceEquals(translatedPicture.Source,translated)&&Convert.ToString(action.Header)=="显示原图","right-click shows the cached translated image and exposes the return action");
    Click(translation,"PinTranslation");check(!translatedRecord.ShowTranslation&&ReferenceEquals(translatedPicture.Source,image),"right-click returns to the original image without rerunning translation");
    Field(translation,"translating",true);translation.ContextMenu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent));check(!action.IsEnabled&&Convert.ToString(action.Header)=="正在翻译…","running translation disables repeated submissions in the context menu");
    Field(translation,"translating",false);Call(translation,"RefreshTranslationProvider");var status=(Border)LogicalTreeHelper.FindLogicalNode(translation,"PinTranslationStatus");
    check(action.IsEnabled&&Convert.ToString(action.Header)=="翻译图片"&&((TextBlock)status.Child).Text.Contains("右键")&&!((TextBlock)status.Child).Text.Contains("上方"),"provider changes restore the retry action and instructions refer to the right-click menu");
    check(!window.IsVisible&&!translation.IsVisible&&!window.ContextMenu.IsOpen&&!translation.ContextMenu.IsOpen,"interaction regression opens no pin, context popup, file dialog or text viewer");
    Click(window,"PinClose");window=null;check(!pin.Active&&pin.ClosedAt>0&&File.ReadAllBytes(store.ImagePath(pin)).SequenceEqual(original),"closing through the right-click menu preserves the original pin file and a recoverable history record");
   }finally{if(window!=null)window.Close();if(translation!=null)translation.Close();AppState.Settings=oldSettings;AppState.Pins=oldPins;AppState.Quitting=quitting;}
  }
 }
}
