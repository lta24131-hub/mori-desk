using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Mori;

namespace MoriDesk {
 internal static class DeskSettingsTests {
  static IEnumerable<DependencyObject> All(DependencyObject root){yield return root;foreach(object child in LogicalTreeHelper.GetChildren(root)){var value=child as DependencyObject;if(value!=null)foreach(var nested in All(value))yield return nested;}}
  public static void Run(string root,Action<bool,string> check){
   string settingsPath=Path.Combine(Entry.DataRoot,"settings.json");byte[] before=File.Exists(settingsPath)?File.ReadAllBytes(settingsPath):null;string theme=Theme.Current;
   using(var library=new Library(Path.Combine(root,"settings-library"))){
    var main=new MainWindow(library){PreviewMode=true};new System.Windows.Interop.WindowInteropHelper(main.Window).EnsureHandle();DeskRuntime.Start(true,library.Root);var settings=new DeskSettingsWindow(main.Window,true,library);int windows=Application.Current.Windows.Count;
    try{
     BrowserStatus(main.Window,check);
     check(settings.Section=="general"&&All(settings).OfType<TextBox>().Any(t=>t.Text==library.Root),"settings open directly with actual save-location control");
     check(All(settings).OfType<CheckBox>().Count()==3&&All(settings).OfType<CheckBox>().Count(c=>!c.IsEnabled)==1,"general offers close/trash preferences while startup cannot mutate preview machine");
     foreach(string appearance in new[]{"light","dark"})foreach(string section in new[]{"general","appearance","library","preview","capture","canvas","browser"}){
      Theme.Set(appearance);settings.Select(section);check(Application.Current.Windows.Count==windows,"settings navigation creates no additional window");
      var body=(FrameworkElement)settings.Content;body.Width=674;body.Height=640;body.Measure(new Size(674,640));body.Arrange(new Rect(0,0,674,640));body.UpdateLayout();
      var render=new RenderTargetBitmap(674,640,96,96,PixelFormats.Pbgra32);render.Render(body);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(render));using(var output=File.Create(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"settings-"+section+"-"+appearance+".png")))encoder.Save(output);
      if(section=="capture"){
       var shortcuts=All(settings).OfType<QingJie.ShortcutEditor>().ToArray();
       check(shortcuts.Length==3&&All(settings).OfType<ComboBox>().Any(c=>c.Name=="TranslationProvider"),"shortcut and translation controls embedded directly");
       var buttonStyle=(Style)main.Window.FindResource(typeof(Button));
       check(shortcuts.All(editor=>editor.IsEnabled&&ReferenceEquals(editor.Style,buttonStyle)&&editor.Template!=null),"derived shortcut editors explicitly reuse the host Button style, never preview-disabled native chrome in "+appearance);
       check(shortcuts.All(editor=>ReferenceEquals(editor.Background,Theme.Brush("Control"))&&ReferenceEquals(editor.Foreground,Theme.Brush("Text"))),"shortcut editors bind contrasting live background and text tokens in "+appearance);
       check(shortcuts.All(editor=>{editor.ApplyTemplate();var surface=editor.Template.FindName("body",editor) as Border;return surface!=null&&ReferenceEquals(surface.Background,Theme.Brush("Control"));}),"shortcut editors render the themed host Button surface rather than a native white background in "+appearance);
      }
      if(section=="library")check(All(settings).OfType<BrowseSettingsPanel>().Count()==1&&All(settings).OfType<ComboBox>().Count()==3&&All(settings).OfType<Slider>().Count()==3&&All(settings).OfType<CheckBox>().Count()==4,"browse tab embeds full preferences without opening another window");
      if(section=="browser"){
       check(All(settings).OfType<TextBox>().Any(t=>t.MaxLength==64),"legacy browser ID available inside advanced settings");
       check(All(settings).OfType<Button>().Any(b=>Convert.ToString(b.Content)=="一键更新"),"browser has single action update");
       check(All(settings).OfType<Expander>().All(e=>!e.IsExpanded),"browser install details are collapsed by default");
      }
     }
     settings.Select("library");var compact=(FrameworkElement)settings.Content;compact.Width=644;compact.Height=402;compact.Measure(new Size(644,402));compact.Arrange(new Rect(0,0,644,402));compact.UpdateLayout();
     var tabTitles=new[]{"通用与存储","外观","素材浏览","文件预览","截图与翻译","自由画布","浏览器收藏"};var tabs=All(settings).OfType<Button>().Where(b=>tabTitles.Contains(Convert.ToString(b.Content))).ToArray();
     check(tabs.Length==7&&tabs.All(b=>b.TranslatePoint(new Point(b.ActualWidth,0),compact).X<=644),"all seven settings sections remain inside a compact vertical navigation");
     var browse=All(settings).OfType<BrowseSettingsPanel>().Single();check(browse.ScrollableWidth<.1&&browse.ScrollableHeight>0,"browse options remain accessible through vertical scroll at minimum window height");
     settings.Select("appearance");var panel=All(settings).OfType<AppearanceSettingsPanel>().Single();panel.Mode.SelectedIndex=theme=="light"?1:0;
     var source=System.Windows.Interop.HwndSource.FromHwnd(new System.Windows.Interop.WindowInteropHelper(main.Window).Handle);
     Func<KeyEventArgs> escape=()=>new KeyEventArgs(Keyboard.PrimaryDevice,source,0,Key.Escape){RoutedEvent=Keyboard.KeyDownEvent};
     var handled=escape();handled.Handled=true;settings.DismissOnEscape(handled);
     check(Application.Current.Windows.Count==windows,"settings does not swallow an Escape already used by a child control");
     QingJie.AppState.ShortcutRecorder=key=>{};var recording=escape();settings.DismissOnEscape(recording);QingJie.AppState.ShortcutRecorder=null;
     check(!recording.Handled&&Application.Current.Windows.Count==windows,"Escape cancels shortcut recording before closing settings");
     var close=escape();settings.RaiseEvent(close);
     check(close.Handled&&Application.Current.Windows.Count==windows-1&&Theme.Current==theme,"Escape closes settings and restores unsaved theme preview");
    }finally{settings.Close();DeskRuntime.Stop();main.Window.Close();ThumbnailImage.CurrentLibrary=null;}
    check(Theme.Current==theme,"closing reverts uncommitted appearance preview");
    check(before==null?!File.Exists(settingsPath):File.ReadAllBytes(settingsPath).SequenceEqual(before),"preview settings never change real personal configuration");
   }
  }
  static void BrowserStatus(Window reference,Action<bool,string> check){
   var current=new BrowserExtensionStatus{summary="浏览器已连接，插件已是最新版本。",connected=true,upToDate=true,version="0.2.17",installedVersion="0.2.17",bundledVersion="0.2.17",availableVersion="0.2.17",profiles=1};
   check(BrowserSettingsPresentation.Current(current)&&!BrowserSettingsPresentation.CanUpdate(current),"current confirmed plugin does not offer redundant reload");
   check(BrowserSettingsPresentation.UpdateHint(current).Contains("最新"),"current update button explains why no update is needed");
   current.profiles=2;check(BrowserSettingsPresentation.UpdateHint(current).Contains("最新")&&!BrowserSettingsPresentation.UpdateHint(current).Contains("关闭"),"all profiles already current need no unnecessary browser closure");current.profiles=1;
   current.upToDate=false;check(!BrowserSettingsPresentation.Current(current)&&BrowserSettingsPresentation.CanUpdate(current),"selected install cannot hide another installation needing update");current.upToDate=true;
   var queued=new BrowserExtensionStatus{connected=true,pending=true,version="0.2.16",installedVersion="0.2.16",bundledVersion="0.2.17",profiles=1};
   check(!BrowserSettingsPresentation.CanUpdate(queued)&&BrowserSettingsPresentation.UpdateHint(queued).Contains("不需要重复"),"queued update prevents repeated clicks while waiting for browser ACK");
   queued.retryUpdate=true;check(BrowserSettingsPresentation.CanUpdate(queued)&&BrowserSettingsPresentation.UpdateHint(queued).Contains("超时"),"expired update offers a safe retry instead of permanently disabling action");queued.retryUpdate=false;
   queued.pending=false;check(BrowserSettingsPresentation.CanUpdate(queued),"connected older plugin offers update");
   queued.profiles=2;check(!BrowserSettingsPresentation.CanUpdate(queued)&&BrowserSettingsPresentation.UpdateHint(queued).Contains("标签不需要关闭"),"multiple-profile hint does not tell user to close normal web tabs");
   queued.profiles=1;queued.availableVersion="0.2.18";check(!BrowserSettingsPresentation.CanUpdate(queued),"newer installed plugin cannot be presented as an available downgrade");
   var fresh=new BrowserExtensionStatus{bundledVersion="0.2.17"};check(!BrowserSettingsPresentation.CanUpdate(fresh),"first installation routes user to install rather than update");
   var disconnected=new BrowserExtensionStatus{installedVersion="0.2.16",bundledVersion="0.2.17"};check(BrowserSettingsPresentation.CanUpdate(disconnected),"disconnected older managed plugin can queue update");
   check(BrowserSettingsPresentation.Versions(disconnected).Contains("已安装 0.2.16")&&!BrowserSettingsPresentation.Versions(disconnected).Contains("浏览器运行"),"version text distinguishes installed files from actual browser connection");
   string originalTheme=Theme.Current;var owner=new Window();Theme.Attach(owner,reference);
   try{foreach(string appearance in new[]{"light","dark"}){
    Theme.Set(appearance);foreach(bool pending in new[]{false,true}){
     var state=pending?new BrowserExtensionStatus{summary="更新已准备好，等待当前收藏完成并关闭插件弹窗。",connected=true,pending=true,version="0.2.16",installedVersion="0.2.16",bundledVersion="0.2.17",profiles=1}:current;
     var page=BrowserConnection.CreateContent(owner,true,()=>state);var surface=new Border{Child=page,Width=674,Height=520};surface.SetResourceReference(Border.BackgroundProperty,"Canvas");owner.Content=surface;surface.Measure(new Size(674,520));surface.Arrange(new Rect(0,0,674,520));surface.UpdateLayout();
     var update=All(page).OfType<Button>().Single(b=>Convert.ToString(b.Content)=="一键更新");
     check(!update.IsEnabled&&Convert.ToString(update.ToolTip)==BrowserSettingsPresentation.UpdateHint(state),"fixture presentation is safe and update reason is exposed");
     check(ToolTipService.GetShowOnDisabled(update),"disabled update action still exposes its reason on hover");
     check(All(page).OfType<TextBlock>().Count(t=>t.Visibility==Visibility.Collapsed&&t.Text=="")==1,"empty action feedback consumes no vertical space");
     var bitmap=new RenderTargetBitmap(674,520,96,96,PixelFormats.Pbgra32);bitmap.Render(surface);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using(var output=File.Create(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"browser-"+(pending?"pending":"current")+"-"+appearance+".png")))encoder.Save(output);
    }
   }}finally{owner.Close();Theme.Set(originalTheme);}
  }
 }
}
