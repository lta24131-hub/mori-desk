using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Markup;
using System.Windows.Media;

namespace Mori {
 public static class ThemeTests {
  public static List<string> Run(){
   if(Thread.CurrentThread.GetApartmentState()!=ApartmentState.STA)throw new InvalidOperationException("Theme tests require an STA thread.");
   var log=new List<string>();Action<bool,string> check=delegate(bool ok,string name){if(!ok)throw new Exception("FAIL: "+name);log.Add("PASS: "+name);};
   check(MainWindow.FormatTime(null)=="—"&&MainWindow.FormatTime("")=="—"&&MainWindow.FormatTime("invalid timestamp")=="—","unknown or invalid timestamps display a placeholder");
   string utc="2020-01-02T03:04:05.0000000Z",offset="2020-01-02T11:04:05+08:00";
   check(MainWindow.FormatTime(utc)==new DateTimeOffset(2020,1,2,3,4,5,TimeSpan.Zero).LocalDateTime.ToString("yyyy/MM/dd HH:mm"),"metadata timestamp display uses the local timezone");
   check(MainWindow.FormatTime(utc)==MainWindow.FormatTime(offset),"equivalent timestamp offsets display the same local time");
   var app=Application.Current??new Application();var oldShutdown=app.ShutdownMode;string previous=Theme.Current??"dark";
   app.ShutdownMode=ShutdownMode.OnExplicitShutdown;Window owner=null,dialog=null;
   try{
    Theme.Set("dark");
    using(var stream=Assembly.GetExecutingAssembly().GetManifestResourceStream("Mori.Main.xaml"))owner=(Window)XamlReader.Load(stream);
    Theme.Attach(owner,null);
    var content=(FrameworkElement)owner.Content;content.Measure(new Size(1380,900));content.Arrange(new Rect(0,0,1380,900));content.UpdateLayout();
    var search=(TextBox)owner.FindName("Search");search.Text="typed text stays intact";search.ApplyTemplate();
    var notes=(TextBox)owner.FindName("Notes");notes.ApplyTemplate();
    var combo=(ComboBox)owner.FindName("Sort");combo.ApplyTemplate();
    dialog=new Window();Theme.Attach(dialog,owner);var dialogContent=new StackPanel();dialog.Content=dialogContent;
    var dialogText=new TextBox{Text="dialog input"};dialogContent.Children.Add(dialogText);dialogText.ApplyTemplate();
    var checkbox=new CheckBox{Content="Startup",IsChecked=true};dialogContent.Children.Add(checkbox);checkbox.ApplyTemplate();
    var bar=new ScrollBar{Orientation=Orientation.Vertical,Minimum=0,Maximum=100,ViewportSize=10,Value=20};dialogContent.Children.Add(bar);bar.ApplyTemplate();
    var horizontal=new ScrollBar{Orientation=Orientation.Horizontal,Minimum=0,Maximum=100,ViewportSize=10};dialogContent.Children.Add(horizontal);horizontal.ApplyTemplate();
    var track=bar.Template.FindName("PART_Track",bar)as Track;
    var horizontalTrack=horizontal.Template.FindName("PART_Track",horizontal)as Track;
    check(track!=null&&track.Thumb!=null&&track.Orientation==Orientation.Vertical,"vertical scrollbar has a usable track and thumb");
    check(horizontalTrack!=null&&horizontalTrack.Orientation==Orientation.Horizontal,"horizontal scrollbar follows its orientation");
    check(track.IsDirectionReversed&&!horizontalTrack.IsDirectionReversed&&horizontalTrack.DecreaseRepeatButton.Command==ScrollBar.PageLeftCommand&&horizontalTrack.IncreaseRepeatButton.Command==ScrollBar.PageRightCommand,"horizontal scrollbar uses left/right commands and direction");
    track.Thumb.ApplyTemplate();var thumb=track.Thumb.Template.FindName("ThumbBody",track.Thumb)as Border;
    check(thumb!=null,"scrollbar uses a theme-aware thumb surface");
    Color darkCanvas=ColorOf(Theme.Brush("Canvas"));
    bool menusStayedClosed=true;
    foreach(string theme in new[]{"dark","light","dark"}){
     Theme.Set(theme);content.UpdateLayout();
     // Mori creates a new context menu for each opening. An unopened Popup is
     // outside the window tree and does not receive WPF resource invalidation.
     var menu=Theme.Menu(owner);var item=new MenuItem{Header="Test menu item"};menu.Items.Add(item);menu.ApplyTemplate();menu.Measure(new Size(400,400));menu.Arrange(new Rect(0,0,400,400));menu.UpdateLayout();item.ApplyTemplate();menusStayedClosed=menusStayedClosed&&!menu.IsOpen;
     check(Theme.Current==theme&&Same(owner.Background,Theme.Brush("Canvas"))&&Same(owner.Foreground,Theme.Brush("Text")),theme+" theme updates the main window");
     check(Same(search.Background,Theme.Brush("Input"))&&Same(search.Foreground,Theme.Brush("Text"))&&Same(search.CaretBrush,Theme.Brush("Text")),theme+" theme updates textbox background, text, and caret");
     check(search.Text=="typed text stays intact",theme+" theme preserves the current text edit");
     check(Same(notes.Background,Theme.Brush("Input"))&&Same(combo.Foreground,Theme.Brush("Text")),theme+" theme reaches multiline input and combo text");
     check(Same(menu.Background,Theme.Brush("Surface"))&&Same(menu.Foreground,Theme.Brush("Text"))&&Same(item.Foreground,Theme.Brush("Text")),theme+" theme styles newly created context menus and their items");
     check(Same(thumb.Background,Theme.Brush("ScrollThumb")),theme+" theme updates an existing scrollbar thumb");
     check(Same(dialog.Background,Theme.Brush("Canvas"))&&Same(dialogText.Background,Theme.Brush("Input"))&&Same(dialogText.Foreground,Theme.Brush("Text")),theme+" theme reaches an attached dialog and inherited input style");
     check(Same(checkbox.Foreground,Theme.Brush("Text"))&&((TextBlock)checkbox.Template.FindName("CheckMark",checkbox)).Visibility==Visibility.Visible,theme+" theme reaches the checkbox and preserves its checked state");
     check(Same((Brush)app.Resources[SystemColors.HighlightBrushKey],Theme.Brush("AccentSoft"))&&Same((Brush)app.Resources[SystemColors.HighlightTextBrushKey],Theme.Brush("Text")),theme+" theme updates system selection colors");
     bool readable=true;foreach(string background in new[]{"Canvas","Panel","Surface","Input"})readable=readable&&Contrast(Theme.Brush("Text"),Theme.Brush(background))>=4.5&&Contrast(Theme.Brush("Muted"),Theme.Brush(background))>=4.5;
     check(readable,theme+" primary and secondary text meet 4.5:1 contrast on content surfaces");
     if(theme=="light")check(ColorOf(Theme.Brush("Canvas"))!=darkCanvas,"light and dark themes use different canvas colors");
    }
    check(!owner.IsVisible&&!dialog.IsVisible&&menusStayedClosed,"theme verification never opens a desktop window or menu");
   }finally{
    if(dialog!=null)dialog.Close();if(owner!=null)owner.Close();Theme.Set(previous);app.ShutdownMode=oldShutdown;
   }
   return log;
  }
  static bool Same(Brush a,Brush b){return a is SolidColorBrush&&b is SolidColorBrush&&ColorOf(a)==ColorOf(b);}
  static Color ColorOf(Brush brush){return ((SolidColorBrush)brush).Color;}
  static double Contrast(Brush a,Brush b){double first=Luminance(ColorOf(a)),second=Luminance(ColorOf(b));return (Math.Max(first,second)+0.05)/(Math.Min(first,second)+0.05);}
  static double Luminance(Color color){return 0.2126*Linear(color.R)+0.7152*Linear(color.G)+0.0722*Linear(color.B);}
  static double Linear(byte channel){double value=channel/255.0;return value<=0.04045?value/12.92:Math.Pow((value+0.055)/1.055,2.4);}
 }
}
