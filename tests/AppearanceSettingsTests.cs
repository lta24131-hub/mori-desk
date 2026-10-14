using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Mori;

namespace MoriDesk {
 internal static class AppearanceSettingsTests {
  static readonly string[] Accents={"purple","blue","teal","amber","rose","mono"};
  static readonly string[] Neutrals={"Canvas","Panel","Surface","Input","Control","Border","Divider","Text","Muted","Hover","ScrollThumb","OnPrimary"};
  static Color ColorOf(string key){return ((SolidColorBrush)Theme.Brush(key)).Color;}
  static double Channel(byte channel){double value=channel/255.0;return value<=.04045?value/12.92:Math.Pow((value+.055)/1.055,2.4);}
  static double Luminance(Color color){return .2126*Channel(color.R)+.7152*Channel(color.G)+.0722*Channel(color.B);}
  static double Contrast(string first,string second){double a=Luminance(ColorOf(first)),b=Luminance(ColorOf(second));return (Math.Max(a,b)+.05)/(Math.Min(a,b)+.05);}
  static string Hex(string key){var color=ColorOf(key);return "#"+color.R.ToString("X2")+color.G.ToString("X2")+color.B.ToString("X2");}
  static IEnumerable<DependencyObject> All(DependencyObject root){yield return root;foreach(object child in LogicalTreeHelper.GetChildren(root)){var value=child as DependencyObject;if(value!=null)foreach(var nested in All(value))yield return nested;}}
  static void Apply(string mode,string accent){Theme.Apply(new Settings{Theme=mode,ThemeAccent=accent});}
  static bool BrushIs(Brush brush,string key){var solid=brush as SolidColorBrush;return solid!=null&&solid.Color==ColorOf(key);}
  static T Part<T>(Control control,string name) where T:class{return control.Template==null?null:control.Template.FindName(name,control) as T;}
  static bool PixelIs(BitmapSource bitmap,Point point,string key){
   int x=(int)Math.Floor(point.X),y=(int)Math.Floor(point.Y);if(x<0||y<0||x>=bitmap.PixelWidth||y>=bitmap.PixelHeight)return false;
   var pixel=new byte[4];bitmap.CopyPixels(new Int32Rect(x,y,1,1),pixel,4,0);var color=ColorOf(key);return pixel[0]==color.B&&pixel[1]==color.G&&pixel[2]==color.R&&pixel[3]==color.A;
  }
  static void ModeColors(ComboBox mode,Action<bool,string> check,string label){
   mode.ApplyTemplate();var surface=Part<Border>(mode,"ModeSurface");var selection=Part<ContentPresenter>(mode,"ModeSelection");var arrow=Part<TextBlock>(mode,"ModeArrow");
   var popup=Part<Popup>(mode,"PART_Popup");var popupSurface=Part<Border>(mode,"ModePopupSurface");var scroll=Part<ScrollViewer>(mode,"ModePopupScroll");var items=Part<StackPanel>(mode,"ModeItems");
   check(surface!=null&&BrushIs(surface.Background,"Control")&&BrushIs(surface.BorderBrush,"Border"),label+" paints the effective closed mode surface and border with theme resources");
   check(selection!=null&&arrow!=null&&BrushIs(TextElement.GetForeground(selection),"Text")&&BrushIs(arrow.Foreground,"Text"),label+" paints selected mode text and arrow with the readable theme foreground");
   check(popupSurface!=null&&scroll!=null&&items!=null&&BrushIs(popupSurface.Background,"Control")&&BrushIs(popupSurface.BorderBrush,"Border")&&BrushIs(TextElement.GetForeground(popupSurface),"Text")&&BrushIs(scroll.Background,"Control")&&BrushIs(items.Background,"Control"),label+" paints every popup and list surface with the current neutral palette");
   check(popup!=null&&popup.Child!=null&&!popup.IsOpen,label+" can inspect the dropdown template without opening a desktop popup");
  }
  static void ChoiceColors(ComboBoxItem item,DependencyPropertyKey highlighted,Action<bool,string> check,string label){
   item.IsSelected=false;if(highlighted!=null)item.SetValue(highlighted,false);item.ApplyTemplate();item.Measure(new Size(200,40));item.Arrange(new Rect(0,0,200,40));item.UpdateLayout();
   var surface=Part<Border>(item,"ChoiceSurface");var text=Part<ContentPresenter>(item,"ChoiceText");
   check(surface!=null&&text!=null&&BrushIs(surface.Background,"Control")&&BrushIs(TextElement.GetForeground(text),"Text"),label+" ordinary mode items have effective themed surface and text colors");
   if(highlighted!=null){item.SetValue(highlighted,true);item.UpdateLayout();check(surface!=null&&BrushIs(surface.Background,"Hover"),label+" highlighted mode items use the live neutral hover surface");}
   item.IsSelected=true;item.UpdateLayout();check(surface!=null&&text!=null&&BrushIs(surface.Background,"AccentSoft")&&BrushIs(TextElement.GetForeground(text),"Text"),label+" selected mode items retain readable text on the live selection tint, including when highlighted");
   if(highlighted!=null)item.SetValue(highlighted,false);item.IsSelected=false;item.UpdateLayout();
  }

  internal static void Run(string root,Action<bool,string> check){
   root=Path.Combine(root,"appearance-settings");Directory.CreateDirectory(root);
   string oldData=Environment.GetEnvironmentVariable("MORI_DESK_DATA"),oldMode=Theme.Current,oldAccent=Theme.AccentKey;
   Environment.SetEnvironmentVariable("MORI_DESK_DATA",Path.Combine(root,"isolated-config"));
   try{Palettes(check);Persistence(root,check);Preview(root,check);Draft(root,check);Failure(root,check);Ownership(root,check);ModeRendering(root,check);Presentation(root,check);}
   finally{Environment.SetEnvironmentVariable("MORI_DESK_DATA",oldData);Apply(oldMode,oldAccent);}
  }

  static void Palettes(Action<bool,string> check){
   check(Theme.NormalizeAccent(null)=="purple"&&Theme.NormalizeAccent("")=="purple"&&Theme.NormalizeAccent("unknown")=="purple","missing and unsupported accents safely normalize to the default purple");
   foreach(string accent in Accents)check(Theme.NormalizeAccent(" "+accent.ToUpperInvariant()+" ")==accent,"accent normalization supports case-insensitive known key "+accent);
   var text=new TextBlock();text.SetResourceReference(TextBlock.ForegroundProperty,"Accent");var host=new Window{Content=text};
   try{
    foreach(string mode in new[]{"light","dark"}){
     Apply(mode,"purple");var baseline=Neutrals.ToDictionary(key=>key,key=>ColorOf(key));Color danger=ColorOf("Danger"),error=ColorOf("ErrorText");
     foreach(string accent in Accents){
      Apply(mode,accent);check(Theme.Current==mode&&Theme.AccentKey==accent,"theme keeps mode and accent as independent normalized choices for "+mode+" "+accent);
      foreach(string key in Neutrals){var color=ColorOf(key);check(color==baseline[key]&&color.A==255&&color.R==color.G&&color.G==color.B&&((SolidColorBrush)Theme.Brush(key)).IsFrozen,mode+" "+accent+" keeps "+key+" frozen, opaque and unchanged neutral");}
      check(ColorOf("Danger")==danger&&ColorOf("ErrorText")==error&&error.R>error.G&&error.R>error.B,mode+" "+accent+" retains semantic red error colors");
      foreach(string surface in new[]{"Canvas","Panel","Surface","Input","Control","Hover","AccentSoft"}){
       check(Contrast("Text",surface)>=4.5&&Contrast("Muted",surface)>=4.5,mode+" "+accent+" preserves readable body and secondary text on "+surface);
      }
      check(Contrast("Accent","Canvas")>=4.5&&Contrast("Accent","AccentSoft")>=4.5&&Contrast("OnPrimary","Primary")>=4.5,mode+" "+accent+" has at least 4.5 contrast for accent labels and primary buttons");
      check(Contrast("ErrorText","Canvas")>=4.5&&Contrast("ErrorText","Surface")>=4.5,mode+" "+accent+" keeps readable error text");
      check(ColorOf("Primary")==ColorOf("Accent")&&((SolidColorBrush)text.Foreground).Color==ColorOf("Accent"),mode+" "+accent+" updates primary and existing dynamic-resource controls together");
      check(Application.Current.Resources[SystemColors.HighlightBrushKey]==Theme.Brush("AccentSoft")&&Application.Current.Resources[SystemColors.HighlightTextBrushKey]==Theme.Brush("Text"),mode+" "+accent+" keeps system selection foreground and tint paired");
      Theme.Set(mode=="light"?"dark":"light");check(Theme.AccentKey==accent,"changing mode does not reset the chosen "+accent+" accent");Theme.Set(mode);
     }
    }
    Apply("light","purple");check(Hex("Accent")=="#6D5BC7"&&Hex("AccentSoft")=="#F0EDFA","default light purple palette remains byte-compatible with established theme values");
    Apply("dark","purple");check(Hex("Accent")=="#B7A8ED"&&Hex("AccentSoft")=="#322D40","default dark purple palette remains compatible with established theme values");
    int events=0;Action changed=()=>events++;Theme.Changed+=changed;
    try{Apply("light","teal");check(events==1,"Apply changes mode and accent with exactly one theme notification");Theme.SetAccent("invalid");check(events==2&&Theme.AccentKey=="purple"&&Theme.Current=="light","SetAccent normalizes unsupported keys without changing mode");}
    finally{Theme.Changed-=changed;}
    bool rejected=false;try{Theme.Apply(null);}catch(ArgumentNullException){rejected=true;}check(rejected,"null settings cannot silently masquerade as a successful theme application");
   }finally{host.Close();}
  }

  static void Persistence(string root,Action<bool,string> check){
   string path=Path.Combine(root,"roundtrip.json");check(new Settings().ThemeAccent=="purple","new settings retain the default purple accent");
   foreach(string mode in new[]{"light","dark"})foreach(string accent in Accents){new Settings{LibraryPath=root,Theme=mode,ThemeAccent=accent}.SaveTo(path);var value=Settings.LoadFrom(path);check(value.Theme==mode&&value.ThemeAccent==accent,"settings roundtrip preserves "+mode+" "+accent+" without a new theme-mode value");}
   foreach(string accent in new[]{null,"unknown","../bad"}){var value=new Settings{ThemeAccent=accent};check(value.ThemeAccent=="purple","settings normalize unsupported optional accents");}
   File.WriteAllText(path,new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(new{LibraryPath=root,Theme="light"}));check(Settings.LoadFrom(path).ThemeAccent=="purple","older settings without an accent field upgrade without changing their library");
   var stale=Settings.LoadFrom(path);var current=Settings.LoadFrom(path);current.ThemeAccent="rose";current.SaveTo(path);stale.ThumbnailSize=290;stale.SaveTo(path);check(Settings.LoadFrom(path).ThemeAccent=="rose","a stale unrelated preference editor cannot overwrite a newer accent");
  }

  static void Preview(string root,Action<bool,string> check){
   string path=Path.Combine(root,"preview.json");new Settings{LibraryPath=root,Theme="light",ThemeAccent="blue"}.SaveTo(path);byte[] before=File.ReadAllBytes(path);Apply("light","blue");
   var initial=Settings.LoadFrom(path);var owner=new Window();int writes=0;var panel=new AppearanceSettingsPanel(owner,true,initial,value=>writes++);owner.Content=panel;
   try{
    panel.Mode.SelectedIndex=1;panel.SelectAccent("teal");check(Theme.Current=="dark"&&Theme.AccentKey=="teal","preview controls change the live appearance without a separate window");
    check(initial.Theme=="light"&&initial.ThemeAccent=="blue"&&File.ReadAllBytes(path).SequenceEqual(before),"preview does not mutate the settings editor or its disk file");
    panel.Save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));check(!panel.Save.IsEnabled&&!panel.SaveDraft()&&writes==0&&File.ReadAllBytes(path).SequenceEqual(before),"preview refuses even programmatic persistence");
    panel.Cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));check(Theme.Current=="light"&&Theme.AccentKey=="blue"&&panel.Mode.SelectedIndex==0&&panel.SelectedAccent=="blue","cancel restores both pre-preview mode and accent");
    panel.SelectAccent("rose");owner.Close();check(panel.IsDisposed&&Theme.Current=="light"&&Theme.AccentKey=="blue"&&writes==0,"closing an unsaved preview rolls back both fields and never writes");
    string config=Path.Combine(Entry.DataRoot,"settings.json");check(!File.Exists(config),"isolated preview does not require or create the ordinary settings file");
    var noInitialOwner=new Window();var noInitial=new AppearanceSettingsPanel(noInitialOwner,true);noInitial.SelectAccent("amber");noInitial.Dispose();noInitialOwner.Close();check(!File.Exists(config),"preview without initial settings does not load or save personal settings");
   }finally{panel.Dispose();owner.Close();}
  }

  static void Draft(string root,Action<bool,string> check){
   string path=Path.Combine(root,"draft.json");new Settings{LibraryPath=root,Theme="dark",ThemeAccent="purple",ThumbnailSize=170,RightSidebarWidth=320}.SaveTo(path);Apply("dark","purple");
   var initial=Settings.LoadFrom(path);byte[] before=File.ReadAllBytes(path);int writes=0;var owner=new Window();var panel=new AppearanceSettingsPanel(owner,false,initial,value=>{writes++;value.SaveTo(path);});owner.Content=panel;
   try{
    panel.Mode.SelectedIndex=0;panel.SelectAccent("blue");check(File.ReadAllBytes(path).SequenceEqual(before)&&initial.Theme=="dark"&&initial.ThemeAccent=="purple","appearance controls stay draft-only before saving");
    var newer=Settings.LoadFrom(path);newer.LibraryPath=Path.Combine(root,"newer-library");newer.ThumbnailSize=300;newer.RightSidebarWidth=440;newer.DefaultSort=2;newer.SaveTo(path);
    check(panel.SaveDraft()&&writes==1,"explicit appearance save persists once");var saved=Settings.LoadFrom(path);
    check(saved.Theme=="light"&&saved.ThemeAccent=="blue"&&saved.LibraryPath==newer.LibraryPath&&saved.ThumbnailSize==300&&saved.RightSidebarWidth==440&&saved.DefaultSort==2,"appearance save owns only its changed theme fields and preserves concurrent routing/browse preferences");
    newer=Settings.LoadFrom(path);newer.Theme="dark";newer.ThemeAccent="teal";newer.SaveTo(path);panel.SelectAccent("amber");check(panel.SaveDraft(),"accent-only changes can be saved through an older loaded editor");saved=Settings.LoadFrom(path);
    check(saved.Theme=="dark"&&saved.ThemeAccent=="amber"&&panel.Mode.SelectedIndex==1&&Theme.Current=="dark"&&Theme.AccentKey=="amber","an untouched old mode preserves a newer disk mode and reloads the merged controls");
    before=File.ReadAllBytes(path);panel.Reset.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));check(panel.Mode.SelectedIndex==1&&panel.SelectedAccent=="purple"&&File.ReadAllBytes(path).SequenceEqual(before)&&writes==2,"reset previews only dark/default-purple appearance, without writing or changing unrelated settings");
    panel.RevertDraft();check(Theme.Current=="dark"&&Theme.AccentKey=="amber"&&panel.SelectedAccent=="amber","cancel after reset returns to the last committed appearance, not startup defaults");
    panel.SelectAccent("rose");owner.Close();check(Theme.Current=="dark"&&Theme.AccentKey=="amber"&&File.ReadAllBytes(path).SequenceEqual(before)&&writes==2,"closing after a successful save rolls back only subsequent unsaved preview changes");
   }finally{panel.Dispose();owner.Close();}
  }

  static void Failure(string root,Action<bool,string> check){
   string path=Path.Combine(root,"failure.json");new Settings{LibraryPath=root,Theme="light",ThemeAccent="blue"}.SaveTo(path);byte[] before=File.ReadAllBytes(path);Apply("light","blue");
   var initial=Settings.LoadFrom(path);var owner=new Window();int writes=0;var panel=new AppearanceSettingsPanel(owner,false,initial,value=>{writes++;throw new IOException("synthetic appearance persistence failure");});owner.Content=panel;
   try{
    panel.Mode.SelectedIndex=1;panel.SelectAccent("rose");bool failed=false;try{panel.SaveDraft();}catch(IOException error){failed=error.Message=="synthetic appearance persistence failure";}
    check(failed&&writes==1&&initial.Theme=="light"&&initial.ThemeAccent=="blue"&&File.ReadAllBytes(path).SequenceEqual(before),"a persistence failure commits neither theme editor state nor disk state");
    check(Theme.Current=="dark"&&Theme.AccentKey=="rose"&&panel.Mode.SelectedIndex==1&&panel.SelectedAccent=="rose","failed persistence retains the editable preview for a retry");
    check(All(panel).OfType<TextBlock>().Any(text=>text.Text.Contains("未保存")&&text.Text.Contains("synthetic appearance persistence failure"))&&!All(panel).OfType<TextBlock>().Any(text=>text.Text.Contains("已保存")),"failure is visible without a false save-success message");
    panel.RevertDraft();check(Theme.Current=="light"&&Theme.AccentKey=="blue","cancel after failed persistence still restores the last committed appearance");
    panel.SelectAccent("teal");panel.Dispose();panel.Dispose();check(panel.IsDisposed&&Theme.Current=="light"&&Theme.AccentKey=="blue"&&writes==1&&!panel.SaveDraft(),"disposal is idempotent, rolls back and refuses any later save");
   }finally{panel.Dispose();owner.Close();}
  }

  static void Ownership(string root,Action<bool,string> check){
   Apply("light","purple");var owner=new Window();var panel=new AppearanceSettingsPanel(owner,true,new Settings{LibraryPath=root,Theme="light",ThemeAccent="purple"});owner.Content=panel;
   panel.SelectAccent("blue");Apply("dark","rose");panel.Dispose();owner.Close();check(Theme.Current=="dark"&&Theme.AccentKey=="rose","closing an older preview cannot overwrite an appearance applied by another editor");
  }

  static void ModeRendering(string root,Action<bool,string> check){
   Apply("light","purple");var owner=new Window();var panel=new AppearanceSettingsPanel(owner,true,new Settings{LibraryPath=root,Theme="light",ThemeAccent="purple"});
   // IsHighlighted is read-only. Use its framework key to exercise actual template triggers offscreen, not mouse/desktop input.
   var field=typeof(ComboBoxItem).GetField("IsHighlightedPropertyKey",BindingFlags.Static|BindingFlags.NonPublic);var highlighted=field==null?null:field.GetValue(null) as DependencyPropertyKey;
   check(highlighted!=null,"the targeted WPF framework exposes its read-only highlight key for offscreen item-state testing");
   // Real generated containers must remain in a resource tree to receive application resource invalidations.
   // A separate ComboBox shares the exact production templates without triggering the panel's PreviewDraft during item-state checks.
   var choices=new ComboBox{Template=panel.Mode.Template,ItemContainerStyle=panel.Mode.ItemContainerStyle,Width=200,Height=34};choices.SetResourceReference(Control.BackgroundProperty,"Control");choices.SetResourceReference(Control.ForegroundProperty,"Text");choices.SetResourceReference(Control.BorderBrushProperty,"Border");choices.Items.Add("模式选项");choices.Items.Add("另一选项");
   var host=new StackPanel();host.Children.Add(panel);host.Children.Add(choices);owner.Content=host;
   ComboBoxItem item=null;ControlTemplate template=null;Border surface=null;
   try{
    foreach(var appearance in new[]{new[]{"light","purple"},new[]{"dark","teal"},new[]{"light","rose"}}){
     Apply(appearance[0],appearance[1]);panel.Width=514;panel.Height=360;panel.Measure(new Size(514,360));panel.Arrange(new Rect(0,0,514,360));panel.UpdateLayout();
     string label=appearance[0]+" "+appearance[1]+" mode template refresh";ModeColors(panel.Mode,check,label);
     choices.ApplyTemplate();choices.Measure(new Size(200,34));choices.Arrange(new Rect(0,0,200,34));choices.UpdateLayout();var popup=Part<Popup>(choices,"PART_Popup");
     check(popup!=null&&popup.Child!=null&&!popup.IsOpen,label+" generates test choices while their popup remains closed");
     if(popup!=null&&popup.Child!=null){popup.Child.Measure(new Size(200,200));popup.Child.Arrange(new Rect(new Point(),popup.Child.DesiredSize));popup.Child.UpdateLayout();}
     var generated=choices.ItemContainerGenerator.ContainerFromIndex(0) as ComboBoxItem;
     check(generated!=null&&Object.ReferenceEquals(choices.Template,panel.Mode.Template)&&Object.ReferenceEquals(choices.ItemContainerStyle,panel.Mode.ItemContainerStyle),label+" exercises a real generated container with the unchanged production mode templates");
     if(item==null)item=generated;else check(Object.ReferenceEquals(item,generated),label+" keeps the same generated item in the resource tree across theme changes");
     if(item!=null)ChoiceColors(item,highlighted,check,label);
     check(Theme.Current==appearance[0]&&Theme.AccentKey==appearance[1],label+" item-state checks cannot invoke the appearance editor's preview or alter the chosen palette");
     if(template==null){template=panel.Mode.Template;surface=Part<Border>(panel.Mode,"ModeSurface");}else check(Object.ReferenceEquals(template,panel.Mode.Template)&&Object.ReferenceEquals(surface,Part<Border>(panel.Mode,"ModeSurface")),label+" updates existing template parts without recreating the mode control");
     check(!owner.IsVisible&&!panel.Mode.IsDropDownOpen&&!choices.IsDropDownOpen,"light-dark-light template verification remains entirely offscreen");
    }
   }finally{panel.Dispose();owner.Close();}
  }

  static void Presentation(string root,Action<bool,string> check){
   foreach(string mode in new[]{"light","dark"})foreach(string accent in Accents){
    Apply(mode,accent);var owner=new Window();var panel=new AppearanceSettingsPanel(owner,true,new Settings{LibraryPath=root,Theme=mode,ThemeAccent=accent});owner.Content=panel;
    try{
     check(panel.Mode.Items.Count==2&&panel.AccentButtons.Count==6&&panel.SelectedAccent==accent,"appearance controls expose two modes and six concise color choices");
     foreach(var button in panel.AccentButtons.Values){
      var surface=(SolidColorBrush)button.Background;check(surface.Color.R==surface.Color.G&&surface.Color.G==surface.Color.B&&!String.IsNullOrWhiteSpace(AutomationProperties.GetName(button))&&!String.IsNullOrWhiteSpace(Convert.ToString(button.ToolTip)),"accent choices stay neutral, keyboard-accessible and named rather than flooding the panel with color");
     }
     panel.Width=514;panel.Height=360;panel.Measure(new Size(514,360));panel.Arrange(new Rect(0,0,514,360));panel.UpdateLayout();
     check(!owner.IsVisible&&panel.ScrollableWidth<.1&&panel.ScrollableHeight>0,"minimum-height appearance panel stays unshown and scrolls vertically without horizontal overflow");
     ModeColors(panel.Mode,check,mode+" "+accent+" appearance screenshot");
     var render=new RenderTargetBitmap(514,360,96,96,PixelFormats.Pbgra32);render.Render(panel);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(render));using(var output=File.Create(Path.Combine(root,"appearance-"+mode+"-"+accent+".png")))encoder.Save(output);
     check(PixelIs(render,panel.Mode.TranslatePoint(new Point(6,17),panel),"Control"),mode+" "+accent+" rendered mode face matches the neutral control color rather than a system white face");
     check(!File.Exists(Path.Combine(Entry.DataRoot,"settings.json")),"rendering "+mode+" "+accent+" does not create personal settings");
    }finally{panel.Dispose();owner.Close();}
   }
  }
 }
}
